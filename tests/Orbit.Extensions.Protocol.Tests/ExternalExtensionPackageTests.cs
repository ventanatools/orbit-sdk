using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

using Orbit.Extensions.Protocol;

namespace Orbit.Extensions.Protocol.Tests;

public sealed class ExternalExtensionPackageTests
{
    private const string Manifest = """
        {"schemaVersion":2,"id":"example.timer","name":"Timer","description":"A local timer.",
         "version":"1.2.3","hosts":["orbit"],"contributions":[
          {"id":"example.timer/start","name":"Start","description":"Start this timer.","glyph":"\uE768",
           "capabilities":["invoke","face"],"settings":[
            {"id":"duration","name":"Duration","default":"short","choices":[
             {"value":"short","name":"Short"},{"value":"long","name":"Long"}]}]}]}
        """;
    private const string Descriptor = """{"schemaVersion":1,"protocolVersion":2,"requiredCapabilities":["invoke","face","settings"]}""";

    [Fact]
    public void ValidPackage_IsAnImmutableInertSnapshot()
    {
        var bytes = Archive([new("payload/"), new("payload/companion.exe", [1, 2, 3]), new("payload/src/Main.cs", Utf8("source"))]);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var result = ExternalExtensionPackageReader.Read(bytes);
        Assert.Null(result.Error);
        var package = result.Package!;
        Assert.Equal("example.timer", package.Manifest.Id);
        Assert.Equal(new[] { "invoke", "face", "settings" }, package.Descriptor.RequiredCapabilities);
        Assert.Equal("Start this companion yourself.", package.Readme);
        Assert.Equal(expectedHash, package.Sha256);
        Array.Clear(bytes);
        var file = package.Files.Single(item => item.Path == "payload/companion.exe");
        using var first = file.OpenRead();
        Assert.False(first.CanWrite);
        Assert.False(((MemoryStream)first).TryGetBuffer(out _));
        var body = new byte[file.Length];
        first.ReadExactly(body);
        Assert.Equal(new byte[] { 1, 2, 3 }, body);
        body[0] = 99;
        using var second = file.OpenRead();
        Assert.Equal(1, second.ReadByte());
        var mutate = () => ((IList<ExternalExtensionPackageFile>)package.Files).Clear();
        Assert.Throws<NotSupportedException>(() => mutate());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/payload/file")]
    [InlineData("payload/../outside")]
    [InlineData("payload/./file")]
    [InlineData("payload//file")]
    [InlineData("payload\\file")]
    [InlineData("payload/C:file")]
    [InlineData("payload/file:stream")]
    [InlineData("payload/file.")]
    [InlineData("payload/file ")]
    [InlineData("payload/a b")]
    [InlineData("payload/CON")]
    [InlineData("payload/con.txt")]
    [InlineData("payload/PRN.cs")]
    [InlineData("payload/AUX")]
    [InlineData("payload/NUL.txt")]
    [InlineData("payload/COM1.exe")]
    [InlineData("payload/LPT9.txt")]
    [InlineData("payload/é.txt")]
    [InlineData("Payload/file")]
    [InlineData("extra.json")]
    [InlineData("extension.json/")]
    public void NonportableOrEscapingPaths_AreRefused(string path)
    {
        var result = ExternalExtensionPackageReader.Read(Archive([new(path)]));
        Assert.Null(result.Package);
        Assert.Equal("package.path", result.Error);
        Assert.DoesNotContain(path, result.Error);
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
    public void Collisions_AreRefusedInEitherOrder(string first, string second)
    {
        Assert.Equal("package.path", ExternalExtensionPackageReader.Read(Archive([new(first), new(second)])).Error);
        Assert.Equal("package.path", ExternalExtensionPackageReader.Read(Archive([new(second), new(first)])).Error);
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
    public void LinkDeviceAndConflictingAttributes_AreRefused(uint attributes) =>
        Assert.Equal("package.path", ExternalExtensionPackageReader.Read(Archive([new("payload/file", [], unchecked((int)attributes))])).Error);

    [Fact]
    public void RootsAreMandatory_AndDirectoriesCannotCarryContent()
    {
        foreach (var root in new[] { "extension.json", "package.json", "README.md" })
            Assert.Equal("package.files", ExternalExtensionPackageReader.Read(Zip(Roots().Where(entry => entry.Path != root).Append(new Entry("payload/a")).ToArray())).Error);
        Assert.Equal("package.path", ExternalExtensionPackageReader.Read(Archive([new("payload/folder/", [1])])).Error);
        Assert.NotNull(ExternalExtensionPackageReader.Read(Archive([new("payload/"), new("payload/a/"), new("payload/a/file")])).Package);
        Assert.Equal("package.path", ExternalExtensionPackageReader.Read(Archive([new("payload/" + new string('a', 81))])).Error);
        Assert.Equal("package.path", ExternalExtensionPackageReader.Read(Archive([new("payload/" + string.Join('/', Enumerable.Repeat(new string('a', 60), 4)))])).Error);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":1")]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":3")]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":\"2\"")]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":2,\"entrypoint\":\"run.exe\"")]
    [InlineData("\"invoke\",\"face\",\"settings\"", "\"invoke\",\"invoke\"")]
    [InlineData("\"invoke\",\"face\",\"settings\"", "\"images\"")]
    [InlineData("\"invoke\",\"face\",\"settings\"", "")]
    public void DescriptorRejectsUnknownRepeatedOrUnsupportedRequirements(string before, string after)
    {
        var descriptor = Descriptor.Replace(before, after, StringComparison.Ordinal);
        Assert.Null(ExternalExtensionPackageDescriptorReader.Read(Utf8(descriptor)));
        Assert.Equal("package.requirements", ExternalExtensionPackageReader.Read(Archive(descriptor: descriptor)).Error);
    }

    [Theory]
    [InlineData("\"invoke\",")]
    [InlineData("\"face\",")]
    [InlineData(",\"settings\"")]
    public void RequirementsMustCoverEveryDeclaredFeature(string missing) =>
        Assert.Equal("package.requirements", ExternalExtensionPackageReader.Read(Archive(descriptor: Descriptor.Replace(missing, "", StringComparison.Ordinal))).Error);

    [Fact]
    public void DescriptorSnapshotDetachesMutableInput()
    {
        var source = new List<string> { "invoke", "face", "settings" };
        var snapshot = ExternalExtensionPackageDescriptorReader.Snapshot(new(2, source));
        source.Clear();
        Assert.Equal(new[] { "invoke", "face", "settings" }, snapshot.RequiredCapabilities);
        Assert.Equal(snapshot.RequiredCapabilities, ExternalExtensionPackageDescriptorReader.Read(ExternalExtensionPackageDescriptorReader.Serialize(snapshot))!.RequiredCapabilities);
        var mutate = () => ((IList<string>)snapshot.RequiredCapabilities).Clear();
        Assert.Throws<NotSupportedException>(() => mutate());
    }

    [Fact]
    public void ManifestAndReadmeHaveStrictBounds()
    {
        Assert.Equal("package.manifest", ExternalExtensionPackageReader.Read(Archive(manifest: Manifest.Replace("\"version\":\"1.2.3\"", "\"version\":\"later\"", StringComparison.Ordinal))).Error);
        Assert.Equal("package.readme", ExternalExtensionPackageReader.Read(Archive(readme: [])).Error);
        Assert.Equal("package.readme", ExternalExtensionPackageReader.Read(Archive(readme: [0xFF])).Error);
        Assert.Equal("package.readme", ExternalExtensionPackageReader.Read(Archive(readme: Utf8("text\0text"))).Error);
        Assert.Equal("package.size", ExternalExtensionPackageReader.Read(Archive(readme: new byte[ExternalExtensionPackageReader.MaxReadmeBytes + 1])).Error);
        Assert.Equal("package.size", ExternalExtensionPackageReader.Read(Archive(manifest: new string('a', ExternalExtensionManifestReader.MaxBytes + 1))).Error);
        Assert.Equal("package.size", ExternalExtensionPackageReader.Read(Archive(descriptor: new string('a', ExternalExtensionPackageDescriptorReader.MaxBytes + 1))).Error);
    }

    [Fact]
    public void EntryArchiveAndExpansionLimitsAreEnforced()
    {
        var maximum = Enumerable.Range(0, ExternalExtensionPackageReader.MaxEntries - 3).Select(i => new Entry($"payload/f{i}")).ToArray();
        Assert.NotNull(ExternalExtensionPackageReader.Read(Archive(maximum)).Package);
        Assert.Equal("package.entries", ExternalExtensionPackageReader.Read(Archive([.. maximum, new("payload/extra")])).Error);
        Assert.Equal("package.size", ExternalExtensionPackageReader.Read(new byte[ExternalExtensionPackageReader.MaxArchiveBytes + 1]).Error);
        Assert.Equal("package.size", ExternalExtensionPackageReader.Read(Archive([new("payload/large", new byte[ExternalExtensionPackageReader.MaxFileBytes + 1])])).Error);
        var body = new byte[ExternalExtensionPackageReader.MaxFileBytes];
        Assert.Equal("package.size", ExternalExtensionPackageReader.Read(Archive(Enumerable.Range(0, 4).Select(i => new Entry($"payload/f{i}", body)).ToArray())).Error);
    }

    [Fact]
    public void ForgedLengthAndTruncatedArchivesAreRefused()
    {
        var bytes = Archive([new("payload/body", new byte[ExternalExtensionPackageReader.MaxFileBytes + 1])]);
        for (var at = 0; at <= bytes.Length - 46; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) != 0x02014B50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 28));
            if (at + 46 + nameLength > bytes.Length || Encoding.UTF8.GetString(bytes, at + 46, nameLength) != "payload/body") continue;
            var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 42)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 24), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22), 1);
            break;
        }
        Assert.Null(ExternalExtensionPackageReader.Read(bytes).Package);
        var good = Archive();
        Assert.Null(ExternalExtensionPackageReader.Read(good.AsMemory(0, good.Length - 6)).Package);
        Assert.Equal("package.archive", ExternalExtensionPackageReader.Read(Utf8("not a zip and never an error message")).Error);
    }

    [Fact]
    public void ForgedCountsAndMultiDiskMetadataAreRefusedBeforeHydration()
    {
        var bytes = Archive(Enumerable.Range(0, ExternalExtensionPackageReader.MaxEntries).Select(i => new Entry($"payload/f{i}")).ToArray());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 3);
        Assert.Equal("package.archive", ExternalExtensionPackageReader.Read(bytes).Error);
        bytes = Archive();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 4), 1);
        Assert.Equal("package.archive", ExternalExtensionPackageReader.Read(bytes).Error);
    }

    [Fact]
    public void CorruptedStoredPayload_IsRejected()
    {
        var bytes = Zip([.. Roots(), new("payload/body", [42])], CompressionLevel.NoCompression);
        for (var at = 0; at <= bytes.Length - 30; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) != 0x04034B50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 28));
            if (at + 30 + nameLength > bytes.Length || Encoding.UTF8.GetString(bytes, at + 30, nameLength) != "payload/body") continue;
            bytes[at + 30 + nameLength + extraLength] ^= 1;
            Assert.Equal("package.archive", ExternalExtensionPackageReader.Read(bytes).Error);
            return;
        }
        throw new InvalidOperationException("Test entry was not found.");
    }

    private sealed record Entry(string Path, byte[]? Bytes = null, int? Attributes = null);
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static Entry[] Roots(string manifest = Manifest, string descriptor = Descriptor, byte[]? readme = null) =>
        [new("extension.json", Utf8(manifest)), new("package.json", Utf8(descriptor)), new("README.md", readme ?? Utf8("Start this companion yourself."))];
    private static byte[] Archive(Entry[]? extra = null, string manifest = Manifest, string descriptor = Descriptor, byte[]? readme = null) =>
        Zip([.. Roots(manifest, descriptor, readme), .. extra ?? []]);
    private static byte[] Zip(IReadOnlyList<Entry> entries, CompressionLevel compression = CompressionLevel.Fastest)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, compression);
                if (item.Attributes is { } attributes) entry.ExternalAttributes = attributes;
                using var content = entry.Open();
                content.Write(item.Bytes ?? []);
            }
        return stream.ToArray();
    }
}
