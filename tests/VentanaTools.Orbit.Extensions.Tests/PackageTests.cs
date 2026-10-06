// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Packaging;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

public sealed class PackageTests
{
    private static readonly PackageReadOptions Options = new() { Manifest = TestHosts.Options };

    public static TheoryData<string> Archives()
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("packages/archives.json");
        foreach (var item in document.RootElement.GetProperty("archives").EnumerateArray())
        {
            data.Add(item.GetProperty("file").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Archives))]
    public void EveryGoldenArchiveIsReproducedAndGivesItsExpectedDiagnostics(string file)
    {
        using var document = Fixtures.Json("packages/archives.json");
        var item = document.RootElement.GetProperty("archives").EnumerateArray().Single(entry => entry.GetProperty("file").GetString() == file);
        var spec = ArchiveBuilder.FromJson(item, document.RootElement.GetProperty("contents"));
        var built = ArchiveBuilder.Build(spec);
        var deflated = spec.Entries.Any(entry => entry.Method == 8);
        if (!deflated || Fixtures.Update || !File.Exists(Fixtures.PathOf("packages/" + file)))
        {
            Fixtures.AssertGenerated("packages/" + file, built);
        }

        var archive = Fixtures.Bytes("packages/" + file);
        var result = PackageReader.Read(archive, Options);
        var expected = item.GetProperty("expected").EnumerateArray()
            .Select(entry => (entry.GetProperty("code").GetString()!, entry.GetProperty("path").GetString()!,
                entry.TryGetProperty("file", out var f) ? f.GetString() : null))
            .ToList();
        var actual = result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Path, diagnostic.File)).ToList();
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Count == 0, result.Succeeded);
        if (result.Succeeded)
        {
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archive)), result.Value.Sha256);
            Assert.Equal("example.countdown", result.Value.Manifest.Id);
            Assert.Equal(result.Value.Descriptor.ManifestHash, ManifestWriter.ComputeHash(result.Value.Manifest));
            Assert.Equal(2, result.Value.Descriptor.ArchiveVersion);
            Assert.StartsWith("Start the companion yourself", result.Value.Readme, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnArchiveAtEveryLimitAtOnceIsValid()
    {
        var bytes = Maxima(out var payloadSizes);
        Assert.Equal(PackageReader.MaxArchiveBytes, bytes.Length);
        var result = PackageReader.Read(bytes, Options);
        Assert.Empty(result.Diagnostics);
        var package = result.Value!;
        Assert.Equal(512, package.Files.Count);
        Assert.Equal(64L * 1024 * 1024, package.Files.Sum(file => file.Length));
        Assert.Equal(16L * 1024 * 1024, package.Files.Max(file => file.Length));
        Assert.Equal(65_536, package.Files.Single(file => file.Path == "extension.json").Length);
        Assert.Equal(65_536, package.Files.Single(file => file.Path == "README.md").Length);
        Assert.Equal(64, package.Strings.Count);
        Assert.Contains(package.Files, file => file.Path.Length == 240);
        Assert.True(package.Files.Single(file => file.Path == "extension.package.json").Length <= 256 * 1024);
        Assert.Equal(payloadSizes, package.Files.Where(file => file.Path.StartsWith("payload/", StringComparison.Ordinal)).Sum(file => file.Length));
    }

    [Fact]
    public void LimitsAreEnforcedOneAtATime()
    {
        Assert.Equal(DiagnosticCodes.PackageTooLarge, Assert.Single(PackageReader.Read(new byte[PackageReader.MaxArchiveBytes + 1]).Diagnostics).Code);
        var spec = Valid();
        spec.Entries.Add(new EntrySpec { Name = "payload/large.bin", Content = new byte[16 * 1024 * 1024 + 1], Method = 8 });
        Assert.Contains(PackageReader.Read(ArchiveBuilder.Build(spec), Options).Diagnostics,
            diagnostic => diagnostic is { Code: DiagnosticCodes.PackageFileTooLarge, File: "payload/large.bin" });

        var expanded = Valid();
        for (var i = 0; i < 5; i++)
        {
            expanded.Entries.Add(new EntrySpec { Name = "payload/big" + i + ".bin", Content = new byte[15 * 1024 * 1024], Method = 8 });
        }

        Assert.Equal(DiagnosticCodes.PackageExpandedTooLarge, Assert.Single(PackageReader.Read(ArchiveBuilder.Build(expanded), Options).Diagnostics).Code);

        var entries = Valid();
        for (var i = entries.Entries.Count; i <= 512; i++)
        {
            entries.Entries.Add(new EntrySpec { Name = "payload/f" + i, Content = [] });
        }

        Assert.Equal(513, entries.Entries.Count);
        Assert.Equal(DiagnosticCodes.PackageEntries, Assert.Single(PackageReader.Read(ArchiveBuilder.Build(entries), Options).Diagnostics).Code);
        entries.Entries.RemoveAt(entries.Entries.Count - 1);
        Assert.True(PackageReader.Read(ArchiveBuilder.Build(entries), Options).Succeeded);

        var manifest = Valid();
        manifest.Entries[1].Content = Pad(Fixtures.Text("manifests/valid/countdown.json"), 65_537);
        Assert.Contains(PackageReader.Read(ArchiveBuilder.Build(manifest), Options).Diagnostics,
            diagnostic => diagnostic is { Code: DiagnosticCodes.PackageFileTooLarge, File: "extension.json" });
        var readme = Valid();
        readme.Entries[0].Content = new byte[65_537];
        Assert.Contains(PackageReader.Read(ArchiveBuilder.Build(readme), Options).Diagnostics,
            diagnostic => diagnostic is { Code: DiagnosticCodes.PackageReadme, File: "README.md" });
    }

    [Theory]
    [InlineData("../outside", DiagnosticCodes.PackagePath)]
    [InlineData("/payload/file", DiagnosticCodes.PackagePath)]
    [InlineData("payload/../outside", DiagnosticCodes.PackagePath)]
    [InlineData("payload/./file", DiagnosticCodes.PackagePath)]
    [InlineData("payload//file", DiagnosticCodes.PackagePath)]
    [InlineData("payload\\file", DiagnosticCodes.PackagePath)]
    [InlineData("payload/C:file", DiagnosticCodes.PackagePath)]
    [InlineData("payload/file:stream", DiagnosticCodes.PackagePath)]
    [InlineData("payload/file.", DiagnosticCodes.PackagePath)]
    [InlineData("payload/file ", DiagnosticCodes.PackagePath)]
    [InlineData("payload/a b", DiagnosticCodes.PackagePath)]
    [InlineData("payload/CON", DiagnosticCodes.PackagePath)]
    [InlineData("payload/con.txt", DiagnosticCodes.PackagePath)]
    [InlineData("payload/PRN.cs", DiagnosticCodes.PackagePath)]
    [InlineData("payload/AUX", DiagnosticCodes.PackagePath)]
    [InlineData("payload/NUL.txt", DiagnosticCodes.PackagePath)]
    [InlineData("payload/COM1.exe", DiagnosticCodes.PackagePath)]
    [InlineData("payload/LPT9.txt", DiagnosticCodes.PackagePath)]
    [InlineData("Payload/file", DiagnosticCodes.PackagePathReserved)]
    [InlineData("extra.json", DiagnosticCodes.PackagePathReserved)]
    [InlineData("extension.json/", DiagnosticCodes.PackagePathReserved)]
    [InlineData("strings/de-DE.txt", DiagnosticCodes.PackagePathReserved)]
    public void NonportableEscapingOrMisplacedPathsAreRefused(string path, string code)
    {
        var spec = Valid();
        spec.Entries.Add(new EntrySpec { Name = path, Content = path.EndsWith('/') ? null : [1], Folder = path.EndsWith('/') });
        var result = PackageReader.Read(ArchiveBuilder.Build(spec), Options);
        Assert.Null(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.DoesNotContain(path, diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("payload/a", "payload/a")]
    [InlineData("payload/a", "payload/A")]
    [InlineData("payload/a", "payload/a/b")]
    [InlineData("payload/a/b", "payload/a")]
    [InlineData("payload/a/b", "payload/A/c")]
    [InlineData("payload/a/", "payload/a")]
    [InlineData("payload/a/", "payload/a/")]
    [InlineData("README.md", "payload/file")]
    public void CollisionsAreRefusedInEitherOrder(string first, string second)
    {
        foreach (var (a, b) in new[] { (first, second), (second, first) })
        {
            var spec = Valid();
            spec.Entries.Add(new EntrySpec { Name = a, Content = a.EndsWith('/') ? null : [1], Folder = a.EndsWith('/') });
            spec.Entries.Add(new EntrySpec { Name = b, Content = b.EndsWith('/') ? null : [2], Folder = b.EndsWith('/') });
            var result = PackageReader.Read(ArchiveBuilder.Build(spec), Options);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.PackagePathConflict);
            Assert.Null(result.Value);
        }
    }

    [Theory]
    [InlineData(0xA1FF0000)]
    [InlineData(0x11FF0000)]
    [InlineData(0x21FF0000)]
    [InlineData(0x61FF0000)]
    [InlineData(0xC1FF0000)]
    [InlineData(0x41FF0000)]
    [InlineData(0x00000400)]
    [InlineData(0x00000040)]
    [InlineData(0x00000010)]
    public void LinkDeviceAndConflictingAttributesAreRefused(uint attributes)
    {
        var spec = Valid();
        spec.Entries.Add(new EntrySpec { Name = "payload/file", Content = [], ExternalAttributes = attributes, MadeBy = 0x031E });
        var diagnostic = Assert.Single(PackageReader.Read(ArchiveBuilder.Build(spec), Options).Diagnostics);
        Assert.Equal((DiagnosticCodes.PackageLink, "payload/file"), (diagnostic.Code, diagnostic.File));
    }

    /// <summary>
    /// The descriptor is strict (contract §5.6): unknown, repeated, mistyped, missing and null
    /// members, over-long <c>createdBy</c> values, and an unsorted, repeated or self-listing
    /// inventory are refused. Placeholders: <c>{HASH}</c> the manifest hash; <c>{R0}</c>,
    /// <c>{R1}</c>, <c>{R2}</c> the rows of README.md, extension.json and payload/companion.txt;
    /// <c>{SELF}</c> a row for the descriptor itself; <c>{SHA2}</c> the companion's SHA-256;
    /// <c>{A65}</c> and <c>{A33}</c> strings of 65 and 33 characters. Every expected diagnostic
    /// has file extension.package.json, and the package adds the summary package.descriptor.
    /// </summary>
    [Theory]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}],\"requirements\":[]}", "json.unknown-member /requirements")]
    [InlineData("{\"archiveVersion\":2,\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}]}", "json.duplicate-member /archiveVersion")]
    [InlineData("{\"archiveVersion\":\"2\",\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}]}", "json.type-mismatch /archiveVersion")]
    [InlineData("{\"archiveVersion\":3,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}]}", "schema.version-unsupported /archiveVersion")]
    [InlineData("{\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}]}", "json.required-missing /archiveVersion")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\"}", "json.required-missing /files")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":{}}", "json.type-mismatch /files")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":null,\"files\":[{R0},{R1},{R2}]}", "json.null-not-allowed /manifestHash")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},null,{R2}]}", "json.null-not-allowed /files/1")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"xyz\",\"files\":[{R0},{R1},{R2}]}", "package.manifest-hash /manifestHash")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}],\"createdBy\":{\"name\":\"{A65}\",\"version\":\"1.0.0\"}}", "string.too-long /createdBy/name")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}],\"createdBy\":{\"name\":\"example-tool\",\"version\":\"{A33}\"}}", "string.too-long /createdBy/version")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}],\"createdBy\":{\"name\":\"example-tool\"}}", "json.required-missing /createdBy/version")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}],\"createdBy\":{\"name\":\"example-tool\",\"version\":\"1.0.0\",\"url\":\"https://example.com\"}}", "json.unknown-member /createdBy/url")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R1},{R0},{R2}]}", "package.descriptor /files/1/path")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R0},{R1},{R2}]}", "package.descriptor /files/1/path")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{SELF},{R2}]}", "package.descriptor /files/2/path")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{\"path\":\"payload/companion.txt\",\"size\":15,\"sha256\":\"{SHA2}\",\"mode\":1}]}", "json.unknown-member /files/2/mode")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{\"path\":\"payload/companion.txt\",\"size\":15}]}", "json.required-missing /files/2/sha256")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{\"path\":\"payload/companion.txt\",\"size\":1.5,\"sha256\":\"{SHA2}\"}]}", "json.type-mismatch /files/2/size")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{\"path\":\"payload/companion.txt\",\"size\":-1,\"sha256\":\"{SHA2}\"}]}", "package.descriptor /files/2/size")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{\"path\":\"payload/../companion.txt\",\"size\":15,\"sha256\":\"{SHA2}\"}]}", "package.path /files/2/path")]
    [InlineData("{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{\"path\":\"payload/companion.txt\",\"size\":15,\"sha256\":\"ABC\"}]}", "package.hash-mismatch /files/2/sha256")]
    public void TheDescriptorRefusesUnknownRepeatedMistypedOrMisorderedMembers(string template, string expected)
    {
        var spec = Valid();
        var descriptor = Encoding.UTF8.GetBytes(DescriptorText(spec, template));
        Assert.Null(DescriptorReader.Read(descriptor, new DiagnosticBag()));

        var entry = spec.Entries.FindIndex(item => item.Descriptor);
        spec.Entries[entry] = new EntrySpec { Name = "extension.package.json", Content = descriptor };
        var result = PackageReader.Read(ArchiveBuilder.Build(spec), Options);
        Assert.Null(result.Value);
        var wanted = expected.Split('|')
            .Select(item => item.Split(' '))
            .Select(parts => (parts[0], parts[1], (string?)"extension.package.json"))
            .Append((DiagnosticCodes.PackageDescriptor, string.Empty, null))
            .ToList();
        Assert.Equal(wanted, result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Path, diagnostic.File)));
    }

    [Fact]
    public void TheValidDescriptorTemplateIsAccepted()
    {
        var spec = Valid();
        var descriptor = Encoding.UTF8.GetBytes(DescriptorText(spec,
            "{\"archiveVersion\":2,\"manifestHash\":\"{HASH}\",\"files\":[{R0},{R1},{R2}],\"createdBy\":{\"name\":\"example-tool\",\"version\":\"1.0.0\"}}"));
        var entry = spec.Entries.FindIndex(item => item.Descriptor);
        spec.Entries[entry] = new EntrySpec { Name = "extension.package.json", Content = descriptor };
        var result = PackageReader.Read(ArchiveBuilder.Build(spec), Options);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("example-tool", result.Value!.Descriptor.CreatedBy!.Name);
    }

    [Fact]
    public void MoreThan64StringsFilesAreRefused()
    {
        var strings = Fixtures.Utf8(Fixtures.Text("strings/valid/fr.json"));
        foreach (var count in new[] { 64, 65 })
        {
            var spec = Valid();
            for (var i = 0; i < count; i++)
            {
                var tag = "q" + (char)('a' + (i / 26)) + (char)('a' + (i % 26));
                spec.Entries.Add(new EntrySpec
                {
                    Name = "strings/" + tag + ".json",
                    Content = Encoding.UTF8.GetBytes(strings.Replace("\"fr\"", "\"" + tag + "\"", StringComparison.Ordinal)),
                });
            }

            var result = PackageReader.Read(ArchiveBuilder.Build(spec), Options);
            if (count == 64)
            {
                Assert.Empty(result.Diagnostics);
                Assert.Equal(64, result.Value!.Strings.Count);
            }
            else
            {
                Assert.Null(result.Value);
                var diagnostic = Assert.Single(result.Diagnostics);
                Assert.Equal((DiagnosticCodes.StringsTooManyFiles, string.Empty, (string?)null), (diagnostic.Code, diagnostic.Path, diagnostic.File));
            }
        }
    }

    [Fact]
    public void AllowedAttributesAreAcceptedAndNeverExposed()
    {
        var spec = Valid();
        spec.Entries.Add(new EntrySpec { Name = "payload/hidden.txt", Content = [1], ExternalAttributes = 0x01 | 0x02 | 0x04 | 0x20 | 0x80 });
        spec.Entries.Add(new EntrySpec { Name = "payload/unix.txt", Content = [1], ExternalAttributes = 0x81A40000, MadeBy = 0x031E });
        spec.Entries.Add(new EntrySpec { Name = "payload/folder/", Folder = true, ExternalAttributes = 0x41ED0010, MadeBy = 0x031E });
        Assert.Empty(PackageReader.Read(ArchiveBuilder.Build(spec), Options).Diagnostics);
    }

    [Fact]
    public void ValidPackagesAreImmutableInertSnapshots()
    {
        var bytes = ArchiveBuilder.Build(Valid());
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var package = PackageReader.Read(bytes, Options).Value!;
        Array.Clear(bytes);
        Assert.Equal(expectedHash, package.Sha256);
        var file = package.Files.Single(item => item.Path == "payload/companion.txt");
        using var first = file.OpenRead();
        Assert.False(first.CanWrite);
        var body = new byte[file.Length];
        first.ReadExactly(body);
        body[0] = 99;
        using var second = file.OpenRead();
        Assert.Equal((int)'T', second.ReadByte());
        Assert.Throws<NotSupportedException>(() => ((IList<PackageFile>)package.Files).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PackageFileEntry>)package.Descriptor.Files).Clear());
    }

    [Fact]
    public void ForgedLengthsAndTruncatedArchivesAreRefused()
    {
        var good = ArchiveBuilder.Build(Valid());
        Assert.Equal(DiagnosticCodes.PackageArchive, Assert.Single(PackageReader.Read(good.AsMemory(0, good.Length - 6), Options).Diagnostics).Code);
        Assert.Equal(DiagnosticCodes.PackageArchive, Assert.Single(PackageReader.Read(Encoding.UTF8.GetBytes("not a zip and never an error message"), Options).Diagnostics).Code);
        Assert.Equal(DiagnosticCodes.PackageArchive, Assert.Single(PackageReader.Read(ReadOnlyMemory<byte>.Empty, Options).Diagnostics).Code);
        var forged = (byte[])good.Clone();
        var end = forged.Length - 22;
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(end + 12), BinaryPrimitives.ReadUInt32LittleEndian(forged.AsSpan(end + 12)) + 1);
        Assert.Equal(DiagnosticCodes.PackageArchive, Assert.Single(PackageReader.Read(forged, Options).Diagnostics).Code);
        var counted = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(counted.AsSpan(end + 8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(counted.AsSpan(end + 10), 3);
        Assert.Equal(DiagnosticCodes.PackageArchive, Assert.Single(PackageReader.Read(counted, Options).Diagnostics).Code);
    }

    [Fact]
    public void CorruptedStoredAndDeflatedDataIsRefused()
    {
        foreach (var method in new[] { 0, 8 })
        {
            var spec = Valid();
            spec.Entries.Add(new EntrySpec { Name = "payload/body.bin", Content = Enumerable.Repeat((byte)42, 4096).ToArray(), Method = method });
            var bytes = ArchiveBuilder.Build(spec);
            var name = Encoding.ASCII.GetBytes("payload/body.bin");
            var at = -1;
            for (var i = 30; i <= bytes.Length - name.Length; i++)
            {
                if (bytes.AsSpan(i, name.Length).SequenceEqual(name) && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i - 30)) == 0x04034B50)
                {
                    at = i;
                    break;
                }
            }

            bytes[at + name.Length + 5] ^= 0x5A;
            var result = PackageReader.Read(bytes, Options);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic is { Code: DiagnosticCodes.PackageCrc, File: "payload/body.bin" });
        }
    }

    [Fact]
    public void ThePackageWriterIsDeterministicAndReadable()
    {
        var contents = new PackageContents
        {
            Manifest = Fixtures.Text("manifests/valid/countdown.json"),
            Readme = Encoding.UTF8.GetBytes("Start the companion yourself.\n"),
            Strings = [new PackageContentFile { Path = "de-DE.json", Content = Fixtures.Text("strings/valid/de-DE.json") }],
            Payload =
            [
                new PackageContentFile { Path = "companion/Countdown.txt", Content = Enumerable.Repeat((byte)'a', 10_000).ToArray() },
                new PackageContentFile { Path = "random.bin", Content = RandomNumberGenerator.GetBytes(100) },
                new PackageContentFile { Path = "empty.txt", Content = Array.Empty<byte>() },
            ],
            CreatedBy = new PackageCreator { Name = "example-tool", Version = "1.0.0" },
        };
        var bytes = PackageWriter.Write(contents);
        Assert.Equal(bytes, PackageWriter.Write(contents));
        var result = PackageReader.Read(bytes, Options);
        Assert.Empty(result.Diagnostics);
        var package = result.Value!;
        Assert.Equal("example-tool", package.Descriptor.CreatedBy!.Name);
        Assert.Single(package.Strings);
        Assert.Equal(
            ["README.md", "extension.json", "payload/companion/Countdown.txt", "payload/empty.txt", "payload/random.bin", "strings/de-DE.json"],
            package.Descriptor.Files.Select(file => file.Path));
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal), archive.Entries.Select(entry => entry.FullName));
        Assert.All(archive.Entries, entry =>
        {
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
            Assert.Equal(entry.FullName.EndsWith('/') ? 0x10 : 0, entry.ExternalAttributes);
        });
        Assert.Contains(archive.Entries, entry => entry.FullName == "payload/companion/");
        var descriptor = Encoding.UTF8.GetString(package.Files.Single(file => file.Path == "extension.package.json").OpenRead().ReadAllBytes());
        Assert.StartsWith("{\n  \"archiveVersion\": 2,\n  \"manifestHash\": \"1f7c38cf", descriptor, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageWriterRefusesInvalidManifestsAndPaths()
    {
        var manifest = Fixtures.Text("manifests/valid/countdown.json");
        var readme = Encoding.UTF8.GetBytes("Readme.\n");
        Assert.Throws<ArgumentException>(() => PackageWriter.Write(new PackageContents { Manifest = Fixtures.Text("manifests/invalid/id-required.json"), Readme = readme }));
        Assert.Throws<ArgumentException>(() => PackageWriter.Write(new PackageContents
        {
            Manifest = manifest, Readme = readme, Payload = [new PackageContentFile { Path = "../escape", Content = Array.Empty<byte>() }],
        }));
        Assert.Throws<ArgumentException>(() => PackageWriter.Write(new PackageContents
        {
            Manifest = manifest, Readme = readme,
            Payload = [new PackageContentFile { Path = "a", Content = Array.Empty<byte>() }, new PackageContentFile { Path = "A", Content = Array.Empty<byte>() }],
        }));
        Assert.Throws<ArgumentException>(() => PackageWriter.Write(new PackageContents
        {
            Manifest = manifest, Readme = readme,
            Payload = [new PackageContentFile { Path = "a", Content = Array.Empty<byte>() }, new PackageContentFile { Path = "a/b", Content = Array.Empty<byte>() }],
        }));
        Assert.Throws<ArgumentException>(() => PackageWriter.Write(new PackageContents
        {
            Manifest = manifest, Readme = readme, Strings = [new PackageContentFile { Path = "de/de-DE.json", Content = Array.Empty<byte>() }],
        }));
    }

    [Fact]
    public async Task ReadFileAsyncIsBounded()
    {
        var path = Path.Combine(Path.GetTempPath(), "ventana-s2-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await File.WriteAllBytesAsync(path, ArchiveBuilder.Build(Valid()));
            Assert.True((await PackageReader.ReadFileAsync(path, Options)).Succeeded);
            await using (var stream = File.Create(path))
            {
                stream.SetLength(PackageReader.MaxArchiveBytes + 100);
            }

            Assert.Equal(DiagnosticCodes.PackageTooLarge, Assert.Single((await PackageReader.ReadFileAsync(path, Options)).Diagnostics).Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public void TheZoneOfOriginIsReadAndCopied()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(ZoneOfOrigin.Read(Path.GetTempFileName()));
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "ventana-s2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var package = Path.Combine(folder, "example.zip");
            var extracted = Path.Combine(folder, "extracted.txt");
            File.WriteAllBytes(package, [1]);
            File.WriteAllBytes(extracted, [2]);
            Assert.Null(ZoneOfOrigin.Read(package));
            Assert.Null(ZoneOfOrigin.ReadHostUrl(package));
            foreach (var (stream, zone) in new (string, int)[]
            {
                ("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/x.zip\r\n", 3),
                ("[ZoneTransfer]\r\nZoneId=0\r\n", 0),
                ("[ZoneTransfer]\r\nZoneId=4\r\n", 4),
                ("[ZoneTransfer]\r\nZoneId=9\r\n", 3),
                ("[ZoneTransfer]\r\nZoneId=two\r\n", 3),
                ("[ZoneTransfer]\r\n", 3),
                ("[ZoneTransfer]\r\nZoneId=1\r\n" + new string(' ', 4100), 3),
            })
            {
                File.WriteAllText(package + ":Zone.Identifier", stream);
                Assert.Equal(zone, ZoneOfOrigin.Read(package));
            }

            File.WriteAllText(package + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/x.zip\r\n");
            Assert.Equal("https://example.com/x.zip", ZoneOfOrigin.ReadHostUrl(package));
            ZoneOfOrigin.Apply(extracted, 3, package, ZoneOfOrigin.ReadHostUrl(package));
            var written = File.ReadAllText(extracted + ":Zone.Identifier");
            Assert.Contains("ZoneId=3\r\n", written, StringComparison.Ordinal);
            Assert.Contains("ReferrerUrl=" + new Uri(package).AbsoluteUri + "\r\n", written, StringComparison.Ordinal);
            Assert.Contains("HostUrl=https://example.com/x.zip\r\n", written, StringComparison.Ordinal);
            File.WriteAllText(package + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/a b\r\n");
            Assert.Null(ZoneOfOrigin.ReadHostUrl(package));
            ZoneOfOrigin.Apply(extracted, 3, package, "https://example.com/\nZoneId=0");
            Assert.DoesNotContain("HostUrl", File.ReadAllText(extracted + ":Zone.Identifier"), StringComparison.Ordinal);
            ZoneOfOrigin.Apply(extracted, 3, package, "file:///c:/x");
            Assert.DoesNotContain("HostUrl", File.ReadAllText(extracted + ":Zone.Identifier"), StringComparison.Ordinal);
            var untouched = Path.Combine(folder, "untouched.txt");
            File.WriteAllBytes(untouched, [3]);
            ZoneOfOrigin.Apply(untouched, 0, package, null);
            Assert.Null(ZoneOfOrigin.Read(untouched));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZoneOfOrigin.Apply(untouched, 5, package, null));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    internal static ArchiveSpec Valid() => new()
    {
        Entries =
        [
            new EntrySpec { Name = "README.md", Content = Encoding.UTF8.GetBytes("Start the companion yourself.\n") },
            new EntrySpec { Name = "extension.json", Content = Fixtures.Text("manifests/valid/countdown.json") },
            new EntrySpec { Name = "extension.package.json", Descriptor = true },
            new EntrySpec { Name = "payload/", Folder = true },
            new EntrySpec { Name = "payload/companion.txt", Content = Encoding.UTF8.GetBytes("The companion.\n") },
        ],
    };

    /// <summary>Expands the placeholders of a descriptor template (see the descriptor theory) for <paramref name="spec"/>.</summary>
    private static string DescriptorText(ArchiveSpec spec, string template)
    {
        byte[] Content(string name) => spec.Entries.Single(entry => entry.Name == name).Content!;
        static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
        static string Row(string path, byte[] content) =>
            "{\"path\":\"" + path + "\",\"size\":" + content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"sha256\":\"" + Sha(content) + "\"}";

        using var generated = JsonDocument.Parse(ArchiveBuilder.Descriptor(spec));
        return template
            .Replace("{HASH}", generated.RootElement.GetProperty("manifestHash").GetString(), StringComparison.Ordinal)
            .Replace("{R0}", Row("README.md", Content("README.md")), StringComparison.Ordinal)
            .Replace("{R1}", Row("extension.json", Content("extension.json")), StringComparison.Ordinal)
            .Replace("{R2}", Row("payload/companion.txt", Content("payload/companion.txt")), StringComparison.Ordinal)
            .Replace("{SELF}", Row("extension.package.json", []), StringComparison.Ordinal)
            .Replace("{SHA2}", Sha(Content("payload/companion.txt")), StringComparison.Ordinal)
            .Replace("{A65}", new string('a', 65), StringComparison.Ordinal)
            .Replace("{A33}", new string('1', 33), StringComparison.Ordinal);
    }

    private static byte[] Pad(byte[] json, int length)
    {
        var at = Array.LastIndexOf(json, (byte)'}');
        return [.. json[..at], .. Enumerable.Repeat((byte)' ', length - json.Length), .. json[at..]];
    }

    /// <summary>Builds the archive of fixtures/packages/archives.json "generated": every limit of contract §5.3 at once.</summary>
    private static byte[] Maxima(out long payloadBytes)
    {
        const int megabyte = 1024 * 1024;
        var spec = new ArchiveSpec();
        spec.Entries.Add(new EntrySpec { Name = "README.md", Content = Pad(Encoding.UTF8.GetBytes("{" + new string('R', 10) + "}"), 65_536).Select(b => b == (byte)'{' || b == (byte)'}' ? (byte)'R' : b).ToArray(), Method = 8 });
        spec.Entries.Add(new EntrySpec { Name = "extension.json", Content = Pad(Fixtures.Text("manifests/valid/countdown.json"), 65_536), Method = 8 });
        spec.Entries.Add(new EntrySpec { Name = "extension.package.json", Descriptor = true, Method = 8 });
        var strings = Fixtures.Text("strings/valid/fr.json");
        for (var i = 0; i < 64; i++)
        {
            var tag = "q" + (char)('a' + (i / 26)) + (char)('a' + (i % 26));
            var text = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(strings).Replace("\"fr\"", "\"" + tag + "\"", StringComparison.Ordinal));
            spec.Entries.Add(new EntrySpec { Name = "strings/" + tag + ".json", Content = Pad(text, 65_536), Method = 8 });
        }

        var folder = "payload/" + new string('a', 80) + "/" + new string('b', 80) + "/";
        var payload = new List<EntrySpec>();
        for (var i = 0; i < 445; i++)
        {
            var name = folder + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('c', 66);
            payload.Add(new EntrySpec { Name = name, Content = [], Method = i < 2 ? 0 : 8 });
        }

        spec.Entries.AddRange(payload);
        payload[0].Content = Noise(16 * megabyte);
        const long fixedBytes = 65_536L * 66;
        var noise = 15 * megabyte;
        var descriptorSize = 0L;
        byte[] archive = [];
        for (var pass = 0; pass < 6; pass++)
        {
            payload[1].Content = Noise(noise, 2);
            for (var round = 0; round < 3; round++)
            {
                var remaining = (64L * megabyte) - fixedBytes - descriptorSize - (16 * megabyte) - noise;
                var share = remaining / 443;
                for (var i = 2; i < 445; i++)
                {
                    payload[i].Content = new byte[i == 2 ? remaining - (share * 442) : share];
                }

                descriptorSize = ArchiveBuilder.Descriptor(spec).Length;
            }

            archive = ArchiveBuilder.Build(spec);
            var gap = PackageReader.MaxArchiveBytes - archive.Length;
            if (gap is >= 0 and <= ushort.MaxValue)
            {
                break;
            }

            noise += (int)gap - 32_000;
        }

        payloadBytes = payload.Sum(entry => (long)entry.Content!.Length);
        var comment = (int)(PackageReader.MaxArchiveBytes - archive.Length);
        return ArchiveBuilder.Build(new ArchiveSpec { Entries = spec.Entries, Comment = Enumerable.Repeat((byte)'#', comment).ToArray() });
    }

    /// <summary>Deterministic incompressible bytes: SHA-256 in counter mode.</summary>
    private static byte[] Noise(int length, int seed = 1)
    {
        var output = new byte[length];
        Span<byte> block = stackalloc byte[8];
        for (var i = 0; i * 32 < length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(block, seed);
            BinaryPrimitives.WriteInt32LittleEndian(block[4..], i);
            var hash = SHA256.HashData(block);
            hash.AsSpan(0, Math.Min(32, length - (i * 32))).CopyTo(output.AsSpan(i * 32));
        }

        return output;
    }
}

internal static class StreamExtensions
{
    public static byte[] ReadAllBytes(this Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
